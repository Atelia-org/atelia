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
                    last.RowId, overlay), active.Id));
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

    [Fact]
    public async Task OverlayMemberMustMatchItsFrozenReuseCell() {
        using Fixture fixture = await CreateControlFixture(
            turns: 1,
            activate: false,
            createStore: true
        );
        MaintainerDefinitionRevision second =
            MaintainerDefinitionRevision.Create(
                new LogicalColumnId("case.second"),
                fixture.Family.Digest,
                new ContextHeaderBlockTarget(
                    ContextHeaderCarrier.System,
                    "second",
                    "Second context"
                ),
                fixture.Definition.Capability,
                new MaintainerDeclarativeSpec(
                    "Second question",
                    "Maintain the second context."
                ),
                16 * 1024
            );
        MaintainerDefinitionRevision[] definitions = [
            fixture.Definition,
            second
        ];
        BuildTarget target = BuildTarget.Create(definitions.Select(
            static definition => new BuildTargetColumn(
                definition.LogicalColumnId,
                definition.Digest
            )));
        GridBuildRecipe basis = GridBuildRecipe.CreateFull(
            fixture.TimelineHead.TimelineId,
            fixture.TimelineHead.HeadRowId,
            target
        );
        ForgeActiveRecipe(fixture, second, basis);
        GridBuildRecipe overlay = GridBuildRecipe.CreateOverlay(
            basis,
            fixture.TimelineHead.HeadRowId,
            target,
            [second.LogicalColumnId]
        );
        ForgeActiveRecipe(fixture, second, overlay);
        GridBuildRecipe decoy = GridBuildRecipe.CreateFull(
            fixture.TimelineHead.TimelineId,
            fixture.TimelineHead.HeadRowId,
            target,
            basis.Digest
        );

        CellId expectedReuse;
        CellId replacement;
        RecapRowView overlayHead;
        using (RecapGridStoreHandle store = Assert.IsType<
            RecapGridStoreOpenResult.Opened>(RecapGridStoreFactory.Open(
                fixture.Path
            )).Handle) {
            Dictionary<HistoryRowId, RecapRowView> basisRows =
                BuildV5SourceRows(
                    fixture,
                    store,
                    basis,
                    definitions,
                    baseRows: null,
                    contentPrefix: "basis"
                );
            Dictionary<HistoryRowId, RecapRowView> decoyRows =
                BuildV5SourceRows(
                    fixture,
                    store,
                    decoy,
                    definitions,
                    baseRows: null,
                    contentPrefix: "decoy"
                );
            Dictionary<HistoryRowId, RecapRowView> overlayRows =
                BuildV5SourceRows(
                    fixture,
                    store,
                    overlay,
                    definitions,
                    basisRows,
                    "overlay"
                );
            HistoryRowId headRow = fixture.Rows[^1].Descriptor.RowId;
            expectedReuse = basisRows[headRow].OrderedCells[0].CellId;
            replacement = decoyRows[headRow].OrderedCells[0].CellId;
            overlayHead = overlayRows[headRow];
            Assert.Equal(expectedReuse, overlayHead.OrderedCells[0].CellId);
            Assert.NotEqual(expectedReuse, replacement);
            RecapCellArtifact expected = Assert.IsType<
                RecapGridStoreReadResult<RecapCellArtifact>.Found>(
                store.Reader.ReadCell(expectedReuse)).Value;
            RecapCellArtifact other = Assert.IsType<
                RecapGridStoreReadResult<RecapCellArtifact>.Found>(
                store.Reader.ReadCell(replacement)).Value;
            Assert.Equal(expected.Slot.HistoryRowId, other.Slot.HistoryRowId);
            Assert.Equal(expected.LogicalColumnId, other.LogicalColumnId);
            Assert.Equal(expected.DefinitionDigest, other.DefinitionDigest);
            Assert.NotEqual(expected.Slot.RecipeDigest, other.Slot.RecipeDigest);
            Assert.IsType<RecapGridFulfilledPutResult.Inserted>(
                store.Writer.PutFulfilled(
                    FulfilledViewKey.Create(
                        fixture.Journal.BranchRefId,
                        fixture.TimelineHead,
                        headRow,
                        overlay
                    ),
                    overlayHead.Id
                )
            );
        }

        ExecuteStoreSql(
            fixture.Path,
            "PRAGMA foreign_keys=OFF; UPDATE row_view_member SET cell_id=$replacement WHERE row_result_id=$row AND logical_column_id=$column;",
            ("$replacement", replacement.Value),
            ("$row", overlayHead.Id.Value),
            ("$column", fixture.Definition.LogicalColumnId.Value)
        );
        using RecapGridContextHandle getter = OpenGetter(fixture.Journal);
        RecapGridContextResolveResult.Invalid invalid = Assert.IsType<
            RecapGridContextResolveResult.Invalid>(getter.Resolve(
                fixture.Journal.ReadCurrentHead()!.Value,
                0
            ));
        Assert.Equal("RowViewMembershipMismatch", invalid.Code);
    }

    private static Dictionary<HistoryRowId, RecapRowView> BuildV5SourceRows(
        Fixture fixture,
        RecapGridStoreHandle store,
        GridBuildRecipe recipe,
        IReadOnlyList<MaintainerDefinitionRevision> definitions,
        IReadOnlyDictionary<HistoryRowId, RecapRowView>? baseRows,
        string contentPrefix
    ) {
        var result = new Dictionary<HistoryRowId, RecapRowView>();
        RecapRowView? previous = null;
        foreach ((HistoryTimelineSelectedRow row, int rowIndex) in
                 fixture.Rows.Select((row, index) => (row, index))) {
            HistorySegmentDescriptor descriptor = row.Descriptor;
            RecapCellArtifact? reused = baseRows is null
                ? null
                : Assert.IsType<
                    RecapGridStoreReadResult<RecapCellArtifact>.Found>(
                    store.Reader.ReadCell(
                        baseRows[descriptor.RowId].OrderedCells[0].CellId
                    )).Value;
            RowWorkAssignment[] frozen = definitions.Select(
                (definition, index) => new RowWorkAssignment(
                    definition.LogicalColumnId,
                    index == 0 && reused is not null ? reused.Id : null
                )).ToArray();
            var work = new RowWork(
                new RowWorkKey(
                    fixture.Journal.BranchRefId,
                    descriptor.TimelineId,
                    recipe.Digest,
                    descriptor.RowId
                ),
                recipe.Target,
                descriptor.PreviousRowId,
                previous?.Id,
                frozen
            );
            RowBuildAssignment[] assignments = definitions.Select(
                (definition, index) => index == 0 && reused is not null
                    ? (RowBuildAssignment)new RowBuildAssignment.Reuse(
                        definition.LogicalColumnId,
                        reused
                    )
                    : new RowBuildAssignment.Evaluate(new CellSlot(
                        recipe.Digest,
                        descriptor.RowId,
                        work.WorkId,
                        definition.LogicalColumnId
                    ))).ToArray();
            var coordinate = new RowViewCoordinate(
                fixture.Journal.BranchRefId,
                descriptor.TimelineId,
                descriptor.RowId,
                recipe.Digest,
                recipe.Target.Digest,
                descriptor.PreviousRowId,
                previous?.Id,
                bootstrapCompleted: recipe.Kind == GridBuildRecipeKind.Full
                    || recipe.BootstrapThroughRowId == descriptor.RowId
            );
            RowBuildSpec spec = recipe.Kind == GridBuildRecipeKind.Full
                ? RowBuildSpec.CreateFull(
                    recipe,
                    coordinate,
                    assignments,
                    work
                )
                : RowBuildSpec.CreateOverlayBootstrap(
                    recipe,
                    coordinate,
                    assignments,
                    work
                );
            Assert.IsType<RecapGridRowWorkPutResult.Inserted>(
                store.Writer.PutRowWork(work));
            RecapCellArtifact[] cells = assignments.Select(
                (assignment, index) => assignment switch {
                    RowBuildAssignment.Reuse reuse => reuse.Cell,
                    RowBuildAssignment.Evaluate evaluate => Assert.IsType<
                        RecapGridCellPutResult.Inserted>(store.Writer.PutCell(
                            spec,
                            RecapCellDraft.Create(
                                evaluate.Slot,
                                definitions[index].Digest,
                                RecapCellOutcome.Updated,
                                $"{contentPrefix}-{rowIndex}-{index}",
                                definitions[index].MaxContentUtf8Bytes
                            ))).Winner,
                    _ => throw new InvalidOperationException()
                }).ToArray();
            previous = Assert.IsType<RecapGridRowViewPutResult.Inserted>(
                store.Writer.PutRowView(spec, cells)).Winner;
            result.Add(descriptor.RowId, previous);
        }
        return result;
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
            RowWorkAssignment[] frozen = definitions.Select(
                (definition, index) => new RowWorkAssignment(
                    definition.LogicalColumnId,
                    index == 0 && reused is not null ? reused.Id : null
                )).ToArray();
            var work = new RowWork(
                new RowWorkKey(
                    fixture.Journal.BranchRefId,
                    descriptor.TimelineId,
                    recipe.Digest,
                    descriptor.RowId
                ),
                recipe.Target,
                descriptor.PreviousRowId,
                previous?.Id,
                frozen
            );
            RowBuildAssignment[] assignments = definitions.Select(
                (definition, index) => index == 0 && reused is not null
                    ? (RowBuildAssignment)new RowBuildAssignment.Reuse(
                        definition.LogicalColumnId,
                        reused
                    )
                    : new RowBuildAssignment.Evaluate(new CellSlot(
                        recipe.Digest,
                        descriptor.RowId,
                        work.WorkId,
                        definition.LogicalColumnId
                    ))).ToArray();
            var coordinate = new RowViewCoordinate(fixture.Journal.BranchRefId, descriptor.TimelineId,
                descriptor.RowId, recipe.Digest, recipe.Target.Digest,
                descriptor.PreviousRowId, previous?.Id,
                bootstrapCompleted: baseRows is null || recipe.BootstrapThroughRowId == descriptor.RowId);
            RowBuildSpec spec = baseRows is null
                ? RowBuildSpec.CreateFull(
                    recipe,
                    coordinate,
                    assignments,
                    work
                )
                : RowBuildSpec.CreateOverlayBootstrap(
                    recipe,
                    coordinate,
                    assignments,
                    work
                );
            Assert.IsType<RecapGridRowWorkPutResult.Inserted>(
                store.Writer.PutRowWork(work));
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
