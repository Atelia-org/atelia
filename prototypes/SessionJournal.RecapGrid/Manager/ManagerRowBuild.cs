using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Store;

namespace Atelia.SessionJournal.RecapGrid.Manager;

public sealed partial class RecapGridManager {
    private sealed record DerivedRowPlan(
        RowBuildSpec Spec,
        IReadOnlyList<RecapCellArtifact> PreviousCells
    );

    private (DerivedRowPlan?, RecapGridBuildResult?) DeriveRowPlan(
        FrozenOperation frozen,
        FrozenRecipePlan plan,
        HistoryTimelineSelectedRow selected,
        bool isOverlayBootstrap,
        BuiltRow? previousRow,
        BuiltRow? baseRow,
        bool allowNewWorkSelection = false
    ) {
        HistorySegmentDescriptor descriptor = selected.Descriptor;
        if ((descriptor.PreviousRowId is null) != (previousRow is null)) {
            return (null, Invalid(
                "PreviousCandidateViewMismatch",
                "Candidate row provenance does not match Timeline order."
            ));
        }
        RowResultId? previousRowResultId = null;
        IReadOnlyList<RecapCellArtifact> previousCells = [];
        if (previousRow is not null) {
            if (previousRow.View.HistoryRowId
                    != descriptor.PreviousRowId
                || previousRow.View.RefId != descriptor.RefId
                || previousRow.View.TimelineId != descriptor.TimelineId
                || previousRow.View.RecipeDigest != plan.Recipe.Digest) {
                return (null, Invalid(
                    "PreviousCandidateViewMismatch",
                    "The predecessor assignment differs from the exact selected predecessor."
                ));
            }
            previousCells = previousRow.Cells;
            previousRowResultId = previousRow.View.Id;
        }

        RowBuildAssignment[] provisionalAssignments;
        try {
            provisionalAssignments = DeriveAssignments(
                plan,
                descriptor,
                isOverlayBootstrap,
                baseRow,
                work: null
            );
        }
        catch (Exception exception) when (IsContractFailure(exception)) {
            return (null, Invalid(
                "RowBuildSpecDerivationInvalid",
                exception.Message
            ));
        }
        (RowWork? work, RecapGridBuildResult? workError) = SelectRowWork(
            plan,
            descriptor,
            previousRowResultId,
            provisionalAssignments,
            allowNewWorkSelection
        );
        if (workError is not null) {
            return (null, workError);
        }
        RowBuildAssignment[] assignments;
        try {
            assignments = DeriveAssignments(
                plan,
                descriptor,
                isOverlayBootstrap,
                baseRow,
                work
            );
        }
        catch (Exception exception) when (IsContractFailure(exception)) {
            return (null, Invalid("RowWorkAssignmentDerivationInvalid", exception.Message));
        }
        try {
            var coordinate = new RowViewCoordinate(
                descriptor.RefId,
                descriptor.TimelineId,
                descriptor.RowId,
                plan.Recipe.Digest,
                (work?.ProducerTarget ?? plan.ProducerTarget).Digest,
                descriptor.PreviousRowId,
                previousRowResultId,
                !isOverlayBootstrap
                    || plan.Recipe.BootstrapThroughRowId
                        == descriptor.RowId
            );
            RowBuildSpec spec = plan.Recipe.Kind switch {
                GridBuildRecipeKind.Full => RowBuildSpec.CreateFull(
                    plan.Recipe,
                    coordinate,
                    assignments,
                    work,
                    proposedProducerTarget: plan.ProducerTarget
                ),
                GridBuildRecipeKind.Overlay
                    when isOverlayBootstrap
                    => RowBuildSpec.CreateOverlayBootstrap(
                    plan.Recipe,
                    coordinate,
                    assignments,
                    work,
                    proposedProducerTarget: plan.ProducerTarget
                    ),
                GridBuildRecipeKind.Overlay
                    => RowBuildSpec.CreateNormal(
                    plan.Recipe,
                    coordinate,
                    assignments,
                    work,
                    proposedProducerTarget: plan.ProducerTarget
                    ),
                _ => throw new InvalidOperationException(
                    "The recipe kind is unsupported."
                )
            };
            return (new DerivedRowPlan(
                spec,
                previousCells
            ), null);
        }
        catch (Exception exception) when (IsContractFailure(exception)) {
            return (null, Invalid(
                "RowBuildSpecInvalid",
                exception.Message
            ));
        }
    }

    private RowBuildAssignment[] DeriveAssignments(
        FrozenRecipePlan plan,
        HistorySegmentDescriptor descriptor,
        bool isOverlayBootstrap,
        BuiltRow? baseRow,
        RowWork? work
    ) {
        HashSet<LogicalColumnId> recomputed = plan.Recipe
            .RecomputedColumns.ToHashSet();
        bool overlayBootstrap = plan.Recipe.Kind
                == GridBuildRecipeKind.Overlay
            && isOverlayBootstrap;
        Dictionary<LogicalColumnId, RecapCellArtifact>? reusable = null;
        if (overlayBootstrap) {
            if (baseRow is null) {
                throw new InvalidOperationException(
                    "Overlay bootstrap requires its exact same-row base view."
                );
            }
            reusable = baseRow.Cells.ToDictionary(
                static cell => cell.LogicalColumnId
            );
        }
        BuildTarget producerTarget = work?.ProducerTarget ?? plan.ProducerTarget;
        var assignments = new RowBuildAssignment[
            producerTarget.OrderedColumns.Count
        ];
        for (int index = 0; index < assignments.Length; index++) {
            BuildTargetColumn target =
                producerTarget.OrderedColumns[index];
            if (!overlayBootstrap
                || recomputed.Contains(target.LogicalColumnId)) {
                assignments[index] = new RowBuildAssignment.Evaluate(
                    work is null
                        ? new CellSlot(plan.Recipe.Digest, descriptor.RowId, target.LogicalColumnId)
                        : new CellSlot(plan.Recipe.Digest, descriptor.RowId, work.WorkId, target.LogicalColumnId)
                );
                continue;
            }
            if (!reusable!.TryGetValue(
                    target.LogicalColumnId,
                    out RecapCellArtifact? cell)
                || cell.DefinitionDigest != target.DefinitionDigest
                || cell.Slot.HistoryRowId
                    != descriptor.RowId) {
                throw new InvalidOperationException(
                    "The overlay base view lacks an exact reusable cell."
                );
            }
            assignments[index] = new RowBuildAssignment.Reuse(
                target.LogicalColumnId,
                cell
            );
        }
        return assignments;
    }

    private (RowWork? Work, RecapGridBuildResult? Error) SelectRowWork(
        FrozenRecipePlan plan,
        HistorySegmentDescriptor descriptor,
        RowResultId? previousRowResultId,
        IReadOnlyList<RowBuildAssignment> assignments,
        bool allowNewWorkSelection
    ) {
        var key = new RowWorkKey(
            descriptor.RefId,
            descriptor.TimelineId,
            plan.Recipe.Digest,
            descriptor.RowId
        );
        RecapGridStoreReadResult<RowWork> read = _store.Reader.ReadRowWork(key);
        if (read is RecapGridStoreReadResult<RowWork>.Found found) {
            return (found.Value, null);
        }
        if (read is not RecapGridStoreReadResult<RowWork>.Missing) {
            return (null, MapRowWorkRead(read));
        }
        if (!allowNewWorkSelection) {
            return (null, null);
        }
        var selected = new RowWork(
            key,
            plan.ProducerTarget,
            descriptor.PreviousRowId,
            previousRowResultId,
            assignments.Select(static assignment => assignment switch {
                RowBuildAssignment.Evaluate evaluate => new RowWorkAssignment(evaluate.LogicalColumnId, null),
                RowBuildAssignment.Reuse reuse => new RowWorkAssignment(reuse.LogicalColumnId, reuse.Cell.Id),
                _ => throw new InvalidOperationException("Unsupported RowWork assignment.")
            })
        );
        RecapGridRowWorkPutResult put = _store.Writer.PutRowWork(selected);
        return put switch {
            RecapGridRowWorkPutResult.Inserted inserted => (inserted.Winner, null),
            RecapGridRowWorkPutResult.AlreadyPresent present => (present.Winner, null),
            RecapGridRowWorkPutResult.SelectionConflict conflict => (conflict.Winner, null),
            RecapGridRowWorkPutResult.Busy => (null, Unavailable(RecapGridBuildDependency.Store, "StoreBusy")),
            RecapGridRowWorkPutResult.Disposed => (null, Unavailable(RecapGridBuildDependency.Store, "StoreDisposed")),
            RecapGridRowWorkPutResult.Invalid invalid => (null, Unavailable(RecapGridBuildDependency.Store, invalid.Code, invalid.Detail)),
            _ => (null, Invalid("RowWorkPutOutcomeInvalid", "The Store returned an unknown RowWork outcome."))
        };
    }

    private static RecapGridBuildResult MapRowWorkRead(
        RecapGridStoreReadResult<RowWork> read
    ) => read switch {
        RecapGridStoreReadResult<RowWork>.Busy => Unavailable(RecapGridBuildDependency.Store, "StoreBusy"),
        RecapGridStoreReadResult<RowWork>.Disposed => Unavailable(RecapGridBuildDependency.Store, "StoreDisposed"),
        RecapGridStoreReadResult<RowWork>.Invalid invalid => Unavailable(RecapGridBuildDependency.Store, invalid.Code, invalid.Detail),
        _ => Invalid("RowWorkReadOutcomeInvalid", "The Store returned an unknown RowWork read outcome.")
    };

    private (FrozenRecapCellWork[]?, RecapGridBuildResult?)
        CreateMissingWork(
            FrozenRecipePlan plan,
            RowBuildSpec spec,
            IReadOnlyList<CellSlot> missing
        ) {
        var evaluate = spec.OrderedAssignments
            .OfType<RowBuildAssignment.Evaluate>()
            .ToDictionary(
                static assignment => assignment.Slot
            );
        var seen = new HashSet<CellSlot>();
        var positions = spec.OrderedAssignments
            .Select((assignment, index) => (assignment, index))
            .OfType<(RowBuildAssignment assignment, int index)>()
            .ToDictionary(pair => pair.assignment.LogicalColumnId,
                pair => pair.index);
        var work = new List<FrozenRecapCellWork>(missing.Count);
        int previousPosition = -1;
        foreach (CellSlot key in missing) {
            if (key is null
                || !seen.Add(key)
                || !evaluate.TryGetValue(
                    key,
                    out RowBuildAssignment.Evaluate? assignment)) {
                return (null, Invalid(
                    "MissingCellSlotInvalid",
                    "Store missing keys are not an exact subset of Evaluate assignments."
                ));
            }
            int position = positions[assignment.LogicalColumnId];
            if (position <= previousPosition) {
                return (null, Invalid(
                    "MissingCellSlotOrderInvalid",
                    "Store missing keys are not in target order."
                ));
            }
            previousPosition = position;
            MaintainerDefinitionDigest definitionDigest =
                spec.DefinitionAt(position);
            if (!plan.RegisteredDefinitions.TryGetValue(
                    definitionDigest,
                    out MaintainerDefinitionRevision? definition)
                || definition.LogicalColumnId != assignment.LogicalColumnId
                || !plan.RegisteredFamilies.TryGetValue(
                    definition.FamilyDigest,
                    out FamilyDefinition? family)) {
                return (null, Invalid(
                    "RowWorkDefinitionUnavailable",
                    "The persisted producer definition or family is unavailable."
                ));
            }
            work.Add(new FrozenRecapCellWork(
                position,
                key,
                definition,
                family
            ));
        }
        return (work.ToArray(), null);
    }

    private (RecapCellDraft?, RecapGridBuildResult?) CreateCell(
        FrozenRecapCellWork item,
        RecapCellExecutionOutcome outcome,
        IReadOnlyList<RecapCellArtifact> previousCells
    ) {
        string content;
        RecapCellOutcome cellOutcome;
        switch (outcome) {
            case RecapCellExecutionOutcome.Updated updated:
                content = updated.Content;
                cellOutcome = RecapCellOutcome.Updated;
                break;
            case RecapCellExecutionOutcome.KeepUnchanged:
                RecapCellArtifact? prior = previousCells.SingleOrDefault(
                    cell => cell.LogicalColumnId == item.LogicalColumnId
                );
                if (prior is null) {
                    return (null, Invalid(
                        "KeepUnchangedPriorUnavailable",
                        "KeepUnchanged requires an exact same-column prior cell."
                    ));
                }
                content = prior.Content;
                cellOutcome = RecapCellOutcome.KeepUnchanged;
                break;
            default:
                return (null, new RecapGridBuildResult.ExecutorFailed(
                    "ExecutorOutcomeInvalid",
                    "A successful executor outcome subtype is unsupported."
                ));
        }
        try {
            return (RecapCellDraft.Create(
                item.Slot,
                item.Definition.Digest,
                cellOutcome,
                content,
                item.Definition.MaxContentUtf8Bytes
            ), null);
        }
        catch (Exception exception) when (IsContractFailure(exception)) {
            return (null, new RecapGridBuildResult.ExecutorFailed(
                "ExecutorContentInvalid",
                exception.Message
            ));
        }
    }

    private (RecapCellArtifact?, RecapGridBuildResult?) PutCell(
        FrozenOperation frozen,
        RowBuildSpec spec,
        FrozenRecapCellWork item,
        RecapCellDraft proposed,
        IReadOnlyList<RecapCellArtifact> previousCells,
        BuildState state
    ) {
        if (_store.Identity != frozen.StoreIdentity) {
            return (null, Invalid(
                "StoreIdentityChanged",
                "The Store identity changed during the build operation."
            ));
        }
        RecapGridCellPutResult result = _testHooks.PutCell is null
            ? _store.Writer.PutCell(spec, proposed)
            : _testHooks.PutCell(
                spec,
                proposed,
                () => _store.Writer.PutCell(spec, proposed)
            );
        RecapCellArtifact? winner;
        switch (result) {
            case RecapGridCellPutResult.Inserted inserted:
                state.CellsCommitted++;
                winner = inserted.Winner;
                break;
            case RecapGridCellPutResult.AlreadyFilled already:
                winner = already.Winner;
                break;
            case RecapGridCellPutResult.CommitIndeterminate indeterminate:
                if (indeterminate.IntendedSlot
                    != item.Slot) {
                    return (null, Invalid(
                        "CellSettlementIntendedMismatch",
                        "The indeterminate Cell identity differs from the proposed CellSlot."
                    ));
                }
                winner = indeterminate.Observed;
                if (winner is null) {
                    RecapGridStoreReadResult<RecapCellArtifact> observed =
                        _store.Reader.TryReadCell(item.Slot);
                    if (observed is RecapGridStoreReadResult<
                            RecapCellArtifact>.Found found) {
                        winner = found.Value;
                    }
                    else if (observed is RecapGridStoreReadResult<
                                 RecapCellArtifact>.Missing
                             or RecapGridStoreReadResult<
                                 RecapCellArtifact>.Busy) {
                        return (null, Settlement(
                            RecapGridBuildCommitKind.Cell,
                            indeterminate.IntendedSlot.ToString(),
                            null,
                            state
                        ));
                    }
                    else {
                        return (null, MapStoreCellRead(observed));
                    }
                }
                state.CellsCommitted++;
                break;
            case RecapGridCellPutResult.Busy:
                return (null, Unavailable(
                    RecapGridBuildDependency.Store,
                    "StoreBusy"
                ));
            case RecapGridCellPutResult.Limit limit:
                return (null, Unavailable(
                    RecapGridBuildDependency.Store,
                    "StoreLimit",
                    limit.Name
                ));
            case RecapGridCellPutResult.Disposed:
                return (null, Unavailable(
                    RecapGridBuildDependency.Store,
                    "StoreDisposed"
                ));
            case RecapGridCellPutResult.Rejected rejected:
                return (null, Invalid(
                    "CellRejected",
                    rejected.Code
                ));
            case RecapGridCellPutResult.Invalid invalid:
                return (null, Unavailable(
                    RecapGridBuildDependency.Store,
                    invalid.Code,
                    invalid.Detail
                ));
            default:
                return (null, Invalid(
                    "CellPutOutcomeInvalid",
                    "The Store returned an unknown Cell put outcome."
                ));
        }
        RecapGridBuildResult? validation = ValidateCell(
            winner,
            item.LogicalColumnId,
            item.Definition.Digest,
            item.Definition.MaxContentUtf8Bytes,
            item.Slot,
            previousCells
        );
        return validation is null
            ? (winner, null)
            : (null, validation);
    }

    private (RecapCellArtifact[]?, RecapGridBuildResult?)
        ResolveSelectedCells(
            FrozenRecipePlan plan,
            RowBuildSpec spec,
            IReadOnlyDictionary<CellSlot,
                RecapCellArtifact> settled,
            IReadOnlyList<RecapCellArtifact> previousCells
        ) {
        var cells = new RecapCellArtifact[spec.OrderedAssignments.Count];
        for (int index = 0; index < cells.Length; index++) {
            RowBuildAssignment assignment = spec.OrderedAssignments[index];
            switch (assignment) {
                case RowBuildAssignment.Reuse reuse:
                    cells[index] = reuse.Cell;
                    break;
                case RowBuildAssignment.Evaluate evaluate:
                    if (!settled.TryGetValue(
                            evaluate.Slot,
                            out RecapCellArtifact? cell)) {
                        RecapGridStoreReadResult<RecapCellArtifact> read =
                            _store.Reader.TryReadCell(
                                evaluate.Slot
                            );
                        if (read is not RecapGridStoreReadResult<
                                RecapCellArtifact>.Found found) {
                            return (null, MapStoreCellRead(read));
                        }
                        cell = found.Value;
                    }
                    MaintainerDefinitionDigest definitionDigest =
                        spec.DefinitionAt(index);
                    if (!plan.RegisteredDefinitions.TryGetValue(
                            definitionDigest,
                            out MaintainerDefinitionRevision? definition)
                        || !plan.RegisteredFamilies.ContainsKey(
                            definition.FamilyDigest)) {
                        return (null, Invalid(
                            "RowWorkDefinitionUnavailable",
                            "The persisted producer definition is unavailable."
                        ));
                    }
                    if (definition.LogicalColumnId
                        != assignment.LogicalColumnId) {
                        return (null, Invalid(
                            "RowWorkDefinitionInvalid",
                            "The persisted producer definition does not match the assignment logical column."
                        ));
                    }
                    RecapGridBuildResult? invalid = ValidateCell(
                        cell,
                        assignment.LogicalColumnId,
                        definition.Digest,
                        definition.MaxContentUtf8Bytes,
                        evaluate.Slot,
                        previousCells
                    );
                    if (invalid is not null) {
                        return (null, invalid);
                    }
                    cells[index] = cell;
                    break;
                default:
                    return (null, Invalid(
                        "RowAssignmentInvalid",
                        "The RowBuildSpec contains an unsupported assignment."
                    ));
            }
        }
        return (cells, null);
    }

}
